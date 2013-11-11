using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Serialization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using System.Security.Cryptography;

namespace TyPissed
{
    // Fisher-Yates shuffle extension method for LINQ
    // from: http://stackoverflow.com/questions/1651619/optimal-linq-query-to-get-a-random-sub-collection-shuffle
    public static class EnumerableExtensions
    {
        public static IEnumerable<T> Shuffle<T>(this IEnumerable<T> source)
        {
            return source.Shuffle(new Random());
        }

        public static IEnumerable<T> Shuffle<T>(
            this IEnumerable<T> source, Random rng)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (rng == null) throw new ArgumentNullException("rng");

            return source.ShuffleIterator(rng);
        }

        private static IEnumerable<T> ShuffleIterator<T>(
            this IEnumerable<T> source, Random rng)
        {
            var buffer = source.ToList();
            for (int i = 0; i < buffer.Count; i++)
            {
                int j = rng.Next(i, buffer.Count);
                yield return buffer[j];

                buffer[j] = buffer[i];
            }
        }
    }

    [Serializable]
    // a class representing a big batch of MTurk fuzzer jobs
    public class Job
    {
        private static int FONT_SZ = 14;
        private Dictionary<AST.Address, string> _inputs = new Dictionary<AST.Address, string>();
        private Dictionary<AST.Address, string> _urls = new Dictionary<AST.Address, string>();
        private Dictionary<string, string> _input_url_map = new Dictionary<string, string>();
        private string _id;
        private string _s3bucket;
        private Guid _jobstate;

        public Job(string aws_key, string s3bucket)
        {
            _id = aws_key;
            _s3bucket = s3bucket;
            _jobstate = Guid.NewGuid();
        }

        public string Statistics()
        {
            var mean_length = _inputs.Select(pair => pair.Value.Length).Average();
            var shortest_length = _inputs.Select(pair => pair.Value.Length).Min();
            var longest_length = _inputs.Select(pair => pair.Value.Length).Max();
            var sorted_lengths = _inputs.Select(pair => pair.Value.Length).ToList();
            sorted_lengths.Sort();
            var median_idx = sorted_lengths.Count() / 2; // yes, integer divide
            var median_length = sorted_lengths.ElementAt(median_idx);

            return String.Format("Number of inputs: {0}, Shortest string: {1}\nLongest string: {2}\nMean length: {3}\nMedian length: {4}", _inputs.Count(), shortest_length, longest_length, mean_length, median_length);
        }

        public void AddInput(AST.Address addr, string input)
        {
            if (!_inputs.ContainsKey(addr))
            {
                _inputs.Add(addr, input);
            }
        }

        public void DeserializeInputsFromS3(string input_bucket, Guid input_id, string aws_secret)
        {
            // S3 filename
            var key = "euses_inputs_" + input_id.ToString();

            // download Job from S3
            using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(_id, aws_secret))
            {
                GetObjectRequest getObjectRequest = new GetObjectRequest()
                {
                    BucketName = input_bucket,
                    Key = key
                };

                using (S3Response getObjectResponse = client.GetObject(getObjectRequest))
                {
                    using (Stream s = getObjectResponse.ResponseStream)
                    {
                        // deserialize
                        IFormatter formatter = new BinaryFormatter();
                        _inputs = (Dictionary<AST.Address, string>)formatter.Deserialize(s);
                    }
                }
            }
        }

        public string SerializeToS3(string secret)
        {
            using (MemoryStream stream = new MemoryStream()) {
                // S3 filename
                var key = "state_" + _jobstate.ToString();

                // serialize to memory stream
                IFormatter formatter = new BinaryFormatter();
                formatter.Serialize(stream, this);

                // upload Job to S3
                using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(_id, secret))
                {
                    var tu = new Amazon.S3.Transfer.TransferUtility(client);
                    tu.Upload(stream, _s3bucket, key);
                }
            }

            return _jobstate.ToString();
        }

        public static Job DeserializeFromS3(string bucket, string state_id, string aws_id, string aws_secret)
        {
            Job j;

            // download Job from S3
            using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(aws_id, aws_secret))
            {
                GetObjectRequest getObjectRequest = new GetObjectRequest()
                {
                    BucketName = bucket,
                    Key = "state_" + state_id
                };

                using (S3Response getObjectResponse = client.GetObject(getObjectRequest))
                {
                    using (Stream s = getObjectResponse.ResponseStream)
                    {
                        // deserialize
                        IFormatter formatter = new BinaryFormatter();
                        j = (Job)formatter.Deserialize(s);
                    }
                }
            }
            return j;
        }

        public List<string> GetInternalData()
        {
            var ls = new List<string>();
            foreach (var pair in _inputs)
            {
                ls.Add(String.Format("{0},{1},{2},{3},{4},{5}", _jobstate,_s3bucket,_id,pair.Key.A1FullyQualified(), pair.Value, _urls[pair.Key]));
            }
            return ls;
        }

        public void UploadAllImages(string secret)
        {
            var bitmaps = new Dictionary<string, Bitmap>();
            var biturls = new Dictionary<AST.Address, string>();

            foreach (var pair in _inputs)
            {
                var addr = pair.Key;
                var text = pair.Value;

                // check to see if we've already generated the image for this text
                Bitmap b;
                if (!bitmaps.TryGetValue(text, out b))
                {
                    // create the bitmap
                    b = CreateBitmapImage(text, FONT_SZ);

                    // add to bitmaps dict
                    bitmaps.Add(text, b);
                }
            }

            Console.WriteLine("Uploading {0} images to MTurk for {1} assignments...", bitmaps.Count(), _inputs.Count());

            int progress = 0;
            foreach (var pair in _inputs)
            {
                progress += 1;
                var addr = pair.Key;
                var text = pair.Value;

                Console.Write("\r{0:P}   ", (double)progress / _inputs.Count());

                string url;
                if (!_input_url_map.TryGetValue(text, out url))
                {
                    // get bitmap
                    var b = bitmaps[text];

                    // upload it
                    url = UploadImageToS3(addr, b, secret);

                    // add to map
                    _input_url_map.Add(text, url);
                }
                // add to addr -> url map
                _urls.Add(addr, url);
            }

            Console.Write("\n");
        }

        public void WriteJobToCSV(string filename)
        {
            if (_urls.Count() != _inputs.Count())
            {
                throw new Exception("Need to upload images first.");
            }

            var sb = new StringBuilder();

            // write header
            sb.Append("state_id,path,workbook,worksheet,R,C,original_text,image_url\n");

            // write each input in random order
            foreach (var pair in _inputs.Shuffle())
            {
                var addr = pair.Key;
                var text = pair.Value;
                
                sb.AppendFormat("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\",\"{7}\"\n", _jobstate.ToString(),addr.A1Path(), addr.A1Workbook(), addr.A1Worksheet(), addr.Y, addr.X, text, _urls[addr]);
            }

            // write out to file
            using (var f = File.Create(filename))
            {
                Byte[] data = new UTF8Encoding(true).GetBytes(sb.ToString());
                f.Write(data, 0, data.Length);
            }
        }

        public string GetImageName(AST.Address addr)
        {
            // calculate MD5
            MD5 md5 = MD5.Create();
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(addr.A1FullyQualified());
            byte[] hash = md5.ComputeHash(bytes);

            StringBuilder sb = new StringBuilder();

            // prepend the job state ID
            sb.AppendFormat("{0}", _jobstate.ToString());

            // convert byte array to hex string

            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("X2"));
            }

            // stick a ".png" on the end
            sb.Append(".png");

            // url encode it
            return System.Web.HttpUtility.UrlEncode(sb.ToString());
        }

        public string UploadImageToS3(AST.Address addr, Bitmap b, string secret)
        {
            // convert Bitmap to MemoryStream
            MemoryStream stream = new MemoryStream();
            b.Save(stream, System.Drawing.Imaging.ImageFormat.Png);

            // the image name is the md
            var imagename = GetImageName(addr);

            // the url to the bitmap
            string url;

            // upload MemoryStream to S3
            using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(_id, secret))
            {
                // generate url
                GetPreSignedUrlRequest request = new GetPreSignedUrlRequest()
                {
                    BucketName = _s3bucket,
                    Key = imagename,
                    Verb = HttpVerb.GET,
                    Expires = DateTime.Now.AddMonths(24)
                };
                url = client.GetPreSignedURL(request);

                // upload image
                var tu = new Amazon.S3.Transfer.TransferUtility(client);
                tu.Upload(stream, _s3bucket, imagename);
            }

            return url;
        }

        // borrowed from: http://chiragrdarji.wordpress.com/2008/05/09/generate-image-from-text-using-c-or-convert-text-in-to-image-using-c/
        public static Bitmap CreateBitmapImage(string sImageText, int fontsize)
        {
            Bitmap objBmpImage = new Bitmap(1, 1);

            int intWidth = 0;
            int intHeight = 0;

            // Create the Font object for the image text drawing.
            Font objFont = new Font("Arial", fontsize, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);

            // Create a graphics object to measure the text's width and height.
            Graphics objGraphics = Graphics.FromImage(objBmpImage);

            // This is where the bitmap size is determined.
            intWidth = (int)objGraphics.MeasureString(sImageText, objFont).Width;
            intHeight = (int)objGraphics.MeasureString(sImageText, objFont).Height;

            // Create the bmpImage again with the correct size for the text and font.
            objBmpImage = new Bitmap(objBmpImage, new Size(intWidth, intHeight));

            // Add the colors to the new bitmap.
            objGraphics = Graphics.FromImage(objBmpImage);

            // Set Background color
            objGraphics.Clear(Color.White);
            objGraphics.SmoothingMode = SmoothingMode.AntiAlias;
            objGraphics.TextRenderingHint = TextRenderingHint.AntiAlias;
            objGraphics.DrawString(sImageText, objFont, new SolidBrush(Color.FromArgb(102, 102, 102)), 0, 0);
            objGraphics.Flush();

            return (objBmpImage);
        }

        public static void CleanBucket(string bucket, string aws_id, string aws_secret)
        {
            // set up client
            using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(aws_id, aws_secret))
            {
                // check to ensure that the bucket actually exists
                var dirinfo = new Amazon.S3.IO.S3DirectoryInfo(client, bucket);
                if (dirinfo.Exists)
                {
                    Console.WriteLine("Bucket \"{0}\" already exists.  Erasing.  Sorry if this isn't what you wanted, dude.", bucket);

                    // get a list of the bucket's objects
                    var lor = new ListObjectsRequest
                    {
                        BucketName = bucket
                    };

                    using (ListObjectsResponse r = client.ListObjects(lor))
                    {
                        if (r.S3Objects.Count > 0)
                        {
                            List<KeyVersion> objects = r.S3Objects.Select(obj => new KeyVersion(obj.Key)).ToList();

                            // batch-delete all the objects in the bucket
                            DeleteObjectsRequest dor = new DeleteObjectsRequest
                            {
                                BucketName = bucket,
                                Keys = objects
                            };
                            client.DeleteObjects(dor);
                        }
                    }
                }
                else
                {
                    Console.WriteLine("Creating new bucket \"{0}\"", bucket);

                    // bucket doesn't exist; make a new one
                    PutBucketRequest pbr = new PutBucketRequest
                    {
                        BucketName = bucket
                    };
                    client.PutBucket(pbr);
                }
            }
        }
    }
}