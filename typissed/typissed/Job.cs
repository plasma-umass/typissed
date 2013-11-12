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
        private int _images_per_hit;

        public Job(string aws_key, string s3bucket, int images_per_hit)
        {
            _id = aws_key;
            _s3bucket = s3bucket;
            _jobstate = Guid.NewGuid();
            _images_per_hit = images_per_hit;
        }

        public string Statistics()
        {
            var mean_length = _inputs.Select(pair => pair.Value.Length).Average();
            var shortest_length = _inputs.Select(pair => pair.Value.Length).Min();
            var shortest_string = _inputs.Where(pair => pair.Value.Length == shortest_length).First().Value;
            var longest_length = _inputs.Select(pair => pair.Value.Length).Max();
            var longest_string = _inputs.Where(pair => pair.Value.Length == longest_length).First().Value;
            var sorted_lengths = _inputs.Select(pair => pair.Value.Length).ToList();
            sorted_lengths.Sort();
            var median_idx = sorted_lengths.Count() / 2; // yes, integer divide; this is not precisely correct for even-sized collections
            var median_length = sorted_lengths.ElementAt(median_idx);

            return String.Format("Number of inputs: {0},\nShortest string: \n\tLength: {1}\n\tContents: \"{2}\"\nLongest string:\n\tLength: {3}\n\tContents: \"{4}\"\nMean length: {5}\nMedian length: {6}", _inputs.Count(), shortest_length, shortest_string, longest_length, longest_string, mean_length, median_length);
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

            // temporary storage
            var inputs = new Dictionary<AST.Address, string>();

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
                        inputs = (Dictionary<AST.Address, string>)formatter.Deserialize(s);
                    }
                }
            }

            // sanity check
            System.Diagnostics.Debug.Assert(_inputs.Select(pair => pair.Key).Distinct().Count() == _inputs.Count());

            // remove all leading and trailing whitespace from each string; Turkers will not be able to see it
            var ws = new System.Text.RegularExpressions.Regex(@"\s+");
            inputs = inputs.Select(pair => new KeyValuePair<AST.Address, string>(pair.Key, ws.Replace(pair.Value.Trim(), " "))).ToDictionary(pair => pair.Key, pair => pair.Value);

            // exclude strings that contain zero or more occurrences of only whitespace
            // and replace runs of whitespaces with a single space
            var r = new System.Text.RegularExpressions.Regex(@"^\s*$");
            _inputs = inputs.Where(pair => !r.IsMatch(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        public string SerializeToS3(string secret)
        {
            using (MemoryStream stream = new MemoryStream()) {
                // S3 filename
                var key = "state_" + _jobstate.ToString();

                // serialize to memory stream
                IFormatter formatter = new BinaryFormatter();
                formatter.Serialize(stream, this);

                Console.Error.WriteLine("Upload stream size is: {0} bytes", stream.Length.ToString());

                // upload Job to S3
                using (AmazonS3 client = Amazon.AWSClientFactory.CreateAmazonS3Client(_id, secret))
                {
                    // set the stream position to the start of the stream
                    stream.Position = 0;
                    var tu = new Amazon.S3.Transfer.TransferUtility(client);
                    tu.Upload(stream, _s3bucket, key);
                }
            }

            return _jobstate.ToString();
        }

        public string SerializeToFile()
        {
            // S3 filename
            var filename = "state_" + _jobstate.ToString();
            var path = Path.GetFullPath(filename);

            // serialize to memory stream
            IFormatter formatter = new BinaryFormatter();

            // write to file
            using (Stream stream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                formatter.Serialize(stream, this);
            }

            return path;
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

            Console.WriteLine("Uploading {0} images to MTurk for {1} assignments...", bitmaps.Count(), _inputs.Count() / _images_per_hit);

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
            sb.Append("state_id,");

            // for each image, append header
            for (int i = 0; i < _images_per_hit; i++)
            {
                sb.AppendFormat("path_{0},workbook_{0},worksheet_{0},R_{0},C_{0},original_text_{0},image_url_{0}", i);
                if (i < _images_per_hit - 1)
                {
                    sb.Append(",");
                }
            }
            sb.Append("\n");

            // shuffle inputs
            var inputs = _inputs.Shuffle().ToArray();

            // take in groups of images_per_hit
            for (int i = 0; i < inputs.Length / _images_per_hit; i++ )
            {
                // write each input
                for (int j = 0; j < _images_per_hit; j++)
                {
                    var addr = inputs[i * _images_per_hit + j].Key;
                    var text = inputs[i * _images_per_hit + j].Value;

                    // this is the first image per group
                    if (j % _images_per_hit == 0)
                    {
                        // write job state id
                        sb.AppendFormat("{0},", _jobstate.ToString());
                    }

                    // write image string
                    sb.AppendFormat("\"{0}\",\"{1}\",\"{2}\",\"{3}\",\"{4}\",\"{5}\",\"{6}\"", @addr.A1Path(), @addr.A1Workbook(), @addr.A1Worksheet(), addr.Y, addr.X, text, @_urls[addr]);


                    if (j % _images_per_hit == _images_per_hit - 1)
                    {
                        // this is the last image per group, write newline
                        sb.Append("\n");
                    }
                    else
                    {
                        // otherwise, write comma
                        sb.Append(",");
                    }
                }
            }

            // if there are any remaining images
            // TODO

            // write out to file
            using (var f = File.Create(filename))
            {
                Byte[] data = new UTF8Encoding(true).GetBytes(sb.ToString());
                f.Write(data, 0, data.Length);
            }
        }

        public string WriteTurkTemplate()
        {
            var preamble =
            "<h3>Transcribe the text in the image below.</h3>\n\n" +

            "<div class=\"highlight-box\">\n" +
            "<ul>\n" +
                "\t<li>Please type the text exactly as shown for each of the images including capitalization, spaces, and punctuation.</li>\n" +
                "\t<li>For ease of entry, the TAB key moves to the next field (SHIFT-TAB moves to the previous field).</li>\n" +
                "\t<li>The ENTER key submits the HIT. Don&#39;t hit ENTER when you mean to hit TAB!</li>\n" +
            "</ul>\n" +
            "</div>\n\n";

            string images = "<p>\n";
            for (int i = 0; i < _images_per_hit; i++) {
                images += "\t<img alt=\"If you see this text, type MISSING\" src=\"${image_url_" + i.ToString() + "}\" /><br />\n";
            }
            images += "</p>\n\n";

            var instructions = "<p>Enter the image text in the box below:</p>\n\n";

            string inputs = "<p>\n";
            for (int i = 0; i < _images_per_hit; i++) {
                inputs += "\t<input id=\"input_" + i.ToString() + "\" name=\"input_" + i.ToString() + "\" size=\"30\" tabindex=\"1\" type=\"text\" /><br />\n";
            }
            inputs += "</p>\n\n";

            string postamble =
            "<p>\n" +
            "<style type=\"text/css\"><!--\n" +
            ".highlight-box { border:solid 0px #98BE10; background:#FCF9CE; color:#222222; padding:4px; text-align:left; font-size: smaller;}\n" +
            "-->\n" +
            "</style>\n" +
            "</p>\n" +
            "<script type=\"text/javascript\">\n" +
            "//<![CDATA[\n" +
            "document.getElementById(\"input_0\").focus();\n" +
            "document.getElementById(\"input_0\").select();\n" +
            "//]]>\n" +
            "</script>";

            return preamble + images + instructions + inputs + postamble;
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

        public static AST.Address SimulatedAddress(int i)
        {
            return AST.Address.FromR1C1(i/256, i%256, "simulated", "simulated", "nopath");
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