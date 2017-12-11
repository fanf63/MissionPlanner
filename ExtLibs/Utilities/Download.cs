﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;

namespace MissionPlanner.Utilities
{
    public class DownloadStream : Stream
    {
        private long _length;
        string _uri = "";
        public int chunksize { get; set; } = 1000 * 50;

        private static object _lock = new object();
        /// <summary>
        /// static global cache of instance cache
        /// </summary>
        static readonly Dictionary<string, Dictionary<long, MemoryStream>> _cacheChunks = new Dictionary<string, Dictionary<long, MemoryStream>>();
        /// <summary>
        /// instances
        /// </summary>
        static readonly List<DownloadStream> _instances = new List<DownloadStream>();
        /// <summary>
        /// per instance cache
        /// </summary>
        Dictionary<long,MemoryStream> _chunks = new Dictionary<long, MemoryStream>();

        DateTime _lastread = DateTime.MinValue;

        static void expireCache()
        {
            List<string> seen = new List<string>();
            foreach (var downloadStream in _instances.ToArray())
            {
                // only process a uri once
                if(seen.Contains(downloadStream._uri))
                    continue;
                seen.Add(downloadStream._uri);

                // total instances with this uri
                var uris = _instances.Where(a => { return a._uri == downloadStream._uri; });
                // total instance with thsi uri and old lastread
                var uridates = _instances.Where(a => { return a._uri == downloadStream._uri && a._lastread < DateTime.Now.AddSeconds(-180); });

                // check if they are equal and expire
                if (uris.Count() == uridates.Count())
                {
                    _cacheChunks.Remove(downloadStream._uri);
                    foreach (var uridate in uridates.ToArray())
                    {
                        _instances.Remove(uridate);
                    }
                }
            }
        }

        private static Timer _timer;

        static DownloadStream()
        {
            _timer = new Timer(a => { expireCache(); }, null, 1000 * 30, 1000 * 30);
        }

        public DownloadStream(string uri)
        {
            _uri = uri;
            SetLength(Download.GetFileSize(uri));

            _instances.Add(this);

            lock (_lock)
            {
                if (_cacheChunks.ContainsKey(uri))
                {
                    _chunks = _cacheChunks[uri];
                }
                else
                {
                    _cacheChunks[uri] = _chunks;
                }
            }
        }

        public override void Flush()
        {
        }

        long getChunkNo(long target)
        {
            var t = (long)((target) / chunksize);
            return t;
        }

        long getAlignedChunk(long target)
        {
            return getChunkNo(target) * chunksize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _lastread = DateTime.Now;
            var start = getAlignedChunk(Position);

            // check the cache
            if (!_chunks.ContainsKey(start))
            {
                var end = Math.Min(Length, start + chunksize);

                // cache it
                HttpWebRequest request = (HttpWebRequest) WebRequest.Create(_uri);
                request.AddRange(start, end);
                HttpWebResponse response = (HttpWebResponse) request.GetResponse();

                Console.WriteLine("{0}: {1} - {2}", _uri, start, end);

                MemoryStream ms = new MemoryStream();
                using (Stream stream = response.GetResponseStream())
                {
                    stream.CopyTo(ms);

                    _chunks[start] = ms;
                }
            }

            // return data
            // check to see if this spans a chunk
            if (getChunkNo(Position) != getChunkNo(Position + count-1))
            {
                var bytestoget = count;
                var bytesgot = 0;
                var startchunk = getChunkNo(Position);
                var endchunk = getChunkNo(Position + count - 1);
                for (long chunkno = startchunk; chunkno <= endchunk; chunkno++)
                {
                    var leftinchunk = Position % chunksize == 0 ? chunksize : chunksize - (Position % chunksize);
                    bytesgot += Read(buffer, offset + bytesgot, (int)Math.Min(bytestoget - bytesgot, leftinchunk));
                }
            }
            else
            {
                Array.Copy(_chunks[start].ToArray(), Position - start, buffer, offset, count);

                Position += count;
            }

            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            //Console.WriteLine("Seek: {0} {1}", offset, origin);
            if (origin == SeekOrigin.Begin)
                Position = offset;
            else if (origin == SeekOrigin.Current)
                Position += offset;
            else if (origin == SeekOrigin.End)
                Position = Length + offset;

            return Position;
        }

        public override void SetLength(long value)
        {
            _length = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException("No write");
        }

        public override bool CanRead { get; } = true;
        public override bool CanSeek { get; } = true;
        public override bool CanWrite { get; } = false;
        public override long Length
        {
            get { return _length; }
        }
        public override long Position { get; set; }
    }

    public class Download
    {
        private static readonly ILog log =
            LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public static bool getFilefromNet(string url, string saveto)
        {
            try
            {
                // this is for mono to a ssl server
                //ServicePointManager.CertificatePolicy = new NoCheckCertificatePolicy(); 

                ServicePointManager.ServerCertificateValidationCallback =
                    new System.Net.Security.RemoteCertificateValidationCallback(
                        (sender, certificate, chain, policyErrors) => { return true; });

                log.Info(url);
                // Create a request using a URL that can receive a post. 
                WebRequest request = WebRequest.Create(url);
                request.Timeout = 10000;
                // Set the Method property of the request to POST.
                request.Method = "GET";
                // Get the response.
                WebResponse response = request.GetResponse();
                // Display the status.
                log.Info(((HttpWebResponse)response).StatusDescription);
                if (((HttpWebResponse)response).StatusCode != HttpStatusCode.OK)
                    return false;

                if (File.Exists(saveto))
                {
                    DateTime lastfilewrite = new FileInfo(saveto).LastWriteTime;
                    DateTime lasthttpmod = ((HttpWebResponse)response).LastModified;

                    if (lasthttpmod < lastfilewrite)
                    {
                        if (((HttpWebResponse)response).ContentLength == new FileInfo(saveto).Length)
                        {
                            log.Info("got LastModified " + saveto + " " + ((HttpWebResponse)response).LastModified +
                                     " vs " + new FileInfo(saveto).LastWriteTime);
                            response.Close();
                            return true;
                        }
                    }
                }

                // Get the stream containing content returned by the server.
                Stream dataStream = response.GetResponseStream();

                long bytes = response.ContentLength;
                long contlen = bytes;

                byte[] buf1 = new byte[1024];

                if (!Directory.Exists(Path.GetDirectoryName(saveto)))
                    Directory.CreateDirectory(Path.GetDirectoryName(saveto));

                FileStream fs = new FileStream(saveto + ".new", FileMode.Create);

                DateTime dt = DateTime.Now;

                while (dataStream.CanRead && bytes > 0)
                {
                    log.Debug(saveto + " " + bytes);
                    int len = dataStream.Read(buf1, 0, buf1.Length);
                    bytes -= len;
                    fs.Write(buf1, 0, len);
                }

                fs.Close();
                dataStream.Close();
                response.Close();

                File.Delete(saveto);
                File.Move(saveto + ".new", saveto);

                return true;
            }
            catch (Exception ex)
            {
                log.Info("getFilefromNet(): " + ex.ToString());
                return false;
            }
        }

        public static bool CheckHTTPFileExists(string url)
        {
            bool result = false;

            WebRequest webRequest = WebRequest.Create(url);
            webRequest.Timeout = 1200; // miliseconds
            webRequest.Method = "HEAD";

            HttpWebResponse response = null;

            try
            {
                response = (HttpWebResponse)webRequest.GetResponse();
                result = true;
            }
            catch (WebException webException)
            {
            }
            finally
            {
                if (response != null)
                {
                    response.Close();
                }
            }

            return result;
        }

        //https://stackoverflow.com/questions/13606523/retrieving-partial-content-using-multiple-http-requsets-to-fetch-data-via-parlle
        public static void ParallelDownloadFile(string uri, string filePath, int chunkSize)
        {
            if (uri == null)
                throw new ArgumentNullException("uri");

            // determine file size first
            long size = GetFileSize(uri);

            using (FileStream file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Write))
            {
                file.SetLength(size); // set the length first

                object syncObject = new object(); // synchronize file writes
                Parallel.ForEach(LongRange(0, 1 + size / chunkSize), (start) =>
                {
                    HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
                    request.AddRange(start * chunkSize, start * chunkSize + chunkSize - 1);
                    HttpWebResponse response = (HttpWebResponse)request.GetResponse();

                    lock (syncObject)
                    {
                        using (Stream stream = response.GetResponseStream())
                        {
                            file.Seek(start * chunkSize, SeekOrigin.Begin);
                            stream.CopyTo(file);
                        }
                    }
                });
            }
        }

        public static long GetFileSize(string uri)
        {
            if (uri == null)
                throw new ArgumentNullException("uri");

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "GET";
            HttpWebResponse response = (HttpWebResponse)request.GetResponse();
            var len = response.ContentLength;
            response.Close();
            return len;
        }

        private static IEnumerable<long> LongRange(long start, long count)
        {
            long i = 0;
            while (true)
            {
                if (i >= count)
                {
                    yield break;
                }
                yield return start + i;
                i++;
            }
        }
    }
}