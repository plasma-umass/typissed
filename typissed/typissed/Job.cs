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
    [Serializable]
    // a class representing a big batch of MTurk fuzzer jobs
    public class Job
    {
        private static int FONT_SZ = 14;
        private Dictionary<AST.Address, string> _inputs = new Dictionary<AST.Address, string>();
        private Dictionary<AST.Address, string> _urls = new Dictionary<AST.Address, string>();
        private string _id;
        private string _s3bucket;
        private Guid _jobstate;

        public Job(string aws_key, string s3bucket)
        {
            _id = aws_key;
            _s3bucket = s3bucket;
            _jobstate = Guid.NewGuid();
        }

        public void AddInput(AST.Address addr, string input)
        {
            if (!_inputs.ContainsKey(addr))
            {
                _inputs.Add(addr, input);
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
            foreach (var pair in _inputs)
            {
                var addr = pair.Key;
                var text = pair.Value;

                _urls.Add(addr, UploadImageToS3(addr, CreateBitmapImage(text, FONT_SZ), secret));
            }
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

            // write each input
            foreach (var pair in _inputs)
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
    }
}