using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace rawrequestgen
{
    class Program
    {
        static void Main(string[] args)
        {
            if(args.Length == 0)
            {
                Console.WriteLine("Usage: rawrequestgen.exe example.com request1.txt [--print-request] [--ssl]");
                Console.WriteLine("request1.txt may contain {{bodylength}} which will get replaced with actual body length");
                return;
            }

            bool ssl = args.Contains("--ssl");

            string host = args[0];
            int port = ssl ? 443 : 80;
            if(host.Contains(":")) {
                var a = host.Split(new char[] { ':' });
                host = a[0];
                port = int.Parse(a[1]);
            }

            try
            {
                var request = File.ReadAllBytes(args[1]);
                var origRequestStr = Encoding.ASCII.GetString(request);
                var origHeaderLen = origRequestStr.IndexOf("\r\n\r\n") + 4;
                var origHeader = origRequestStr.Substring(0, origHeaderLen);
                var bodyLen = request.Length - origHeaderLen;
                var newHeader = origHeader.Replace("{{bodylength}}", bodyLen.ToString());
                var newHeaderBytes = Encoding.ASCII.GetBytes(newHeader);
                var newRequest = new byte[newHeaderBytes.Length + bodyLen];
                System.Buffer.BlockCopy(newHeaderBytes, 0, newRequest, 0, newHeaderBytes.Length);
                System.Buffer.BlockCopy(request, origHeaderLen, newRequest, newHeaderBytes.Length, bodyLen);

                if (args.Contains("--print-request-headers"))
                {
                    using (Stream stdout = Console.OpenStandardOutput())
                    {
                        stdout.Write(newHeaderBytes, 0, newHeaderBytes.Length);
                    }
                }

                var stopwatch = new Stopwatch();
                stopwatch.Start();
                var output = requestViaRawTcp(host, port, ssl, newRequest);
                stopwatch.Stop();
                Console.WriteLine(output);
                Console.WriteLine("------------------------------\r\nTime taken: " + stopwatch.ElapsedMilliseconds + " ms");
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }

        private static string requestViaRawTcp(string host, int port, bool ssl, byte[] requestBytes)
        {
            var output = new StringBuilder();

            var c = new TcpClient(host, port);
            Stream tcpStream = c.GetStream();

            var s = tcpStream;
            if(ssl)
            {
                var sslStream = new SslStream(tcpStream, false, new RemoteCertificateValidationCallback((arg1, arg2, arg3, arg4) => true), null);
                sslStream.AuthenticateAsClient(host);
                s = sslStream;
            }

            s.Write(requestBytes, 0, requestBytes.Length);
            s.Flush();

            var sb = new StringBuilder();
            long contentLength = 0;
            bool chunked = false;
            bool responseConnectionClose = false;

            // Read headers
            while (c.Connected)
            {
                int b = s.ReadByte();
                if (b > 0)
                {
                    sb.Append((char)b);
                    if ((char)b == '\n')
                    {
                        var prevLine = sb.ToString();

                        output.Append(prevLine);
                        if (prevLine.StartsWith("Content-Length: "))
                        {
                            contentLength = long.Parse(Regex.Match(prevLine, "Content-Length: (\\d+)").Groups[1].Value);
                        }
                        else if (prevLine.StartsWith("Transfer-Encoding: chunked", StringComparison.InvariantCultureIgnoreCase))
                        {
                            chunked = true;
                        }
                        else if (prevLine.StartsWith("Connection: close", StringComparison.InvariantCultureIgnoreCase))
                        {
                            responseConnectionClose = true;
                        }
                        else if (prevLine.Trim() == "")
                        {
                            break;
                        }

                        sb.Clear();
                    }
                }
                else break;
            }

            if (!chunked)
            {
                // Read normal body as specified by content length
                long curLength = 0;
                while (c.Connected)
                {
                    if (contentLength == 0 && responseConnectionClose)
                    {
                        // server told us to just keep reading until connection is closed
                    }
                    else
                    {
                        if (++curLength > contentLength) break;
                    }

                    int b = s.ReadByte();
                    if (b > 0)
                    {
                        sb.Append((char)b);
                        if ((char)b == '\n')
                        {
                            var prevLine = sb.ToString();
                            output.Append(prevLine);
                            sb.Clear();
                        }
                    }
                    else break;
                }
                if (sb.Length > 0) output.Append(sb.ToString());
            }
            else
            {
                // Read chunked body
                bool done = false;
                while (!done)
                {
                    // read chunk length
                    int chunkLength = 0;
                    while (c.Connected)
                    {
                        int b = s.ReadByte();
                        if (b > 0)
                        {
                            sb.Append((char)b);
                            if ((char)b == '\n')
                            {
                                var prevLine = sb.ToString().Trim().TrimStart(new char[] { '\r', '\n' });
                                chunkLength = int.Parse(prevLine, System.Globalization.NumberStyles.HexNumber);
                                sb.Clear();
                                break;
                            }
                        }
                        else break;
                    }
                    if (chunkLength == 0) break;

                    // read chunk content
                    long curLength = 0;
                    while (c.Connected)
                    {
                        if (++curLength > chunkLength) break;

                        int b = s.ReadByte();
                        if (b > 0)
                        {
                            sb.Append((char)b);
                            if ((char)b == '\n')
                            {
                                var prevLine = sb.ToString();
                                output.Append(prevLine);
                                sb.Clear();
                            }
                        }
                        else break;
                    }

                    // read \r\n after chunk
                    s.ReadByte();
                    s.ReadByte();

                    if (sb.Length > 0) output.Append(sb.ToString());
                    sb.Clear();

                }
            }

            s.Close();
            c.Close();
            return output.ToString();
        }
    }
}
