using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace rawrequestgen
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: rawrequestgen.exe example.com request1.txt [--print-request-headers] [--ssl] [-c] [-s] [-t 5] [-q]");
                Console.WriteLine("-s   Single line output");
                Console.WriteLine("-c   Continuous");
                Console.WriteLine("-t   Concurrent thread count");
                Console.WriteLine("-q   Quiet");
                Console.WriteLine("request1.txt may contain {{bodylength}} which will get replaced with actual body length");
                return;
            }

            bool ssl = args.Contains("--ssl");
            bool continuous = args.Contains("-c");
            bool singleLineOutput = args.Contains("-s");
            bool quiet = args.Contains("-q");

            string host = args[0];
            int port = ssl ? 443 : 80;
            if (host.Contains(":"))
            {
                var a = host.Split(new char[] { ':' });
                host = a[0];
                port = int.Parse(a[1]);
            }

            var threads = 1;
            if (args.Contains("-t"))
            {
                int pos = 0;
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-t") pos = i + 1;
                }
                threads = int.Parse(args[pos]);
            }

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

            double max = -1;
            double avgTime = -1;
            long count = 1;

            for (int i = 0; i < threads; i++)
            {
                new Thread(() =>
                {
                    while (true)
                    {
                        var stopwatch = new Stopwatch();
                        try
                        {
                            stopwatch.Reset();
                            stopwatch.Start();
                            var output = requestViaRawTcp(host, port, ssl, newRequest);
                            stopwatch.Stop();
                            if (continuous || singleLineOutput)
                            {
                                if (avgTime == -1)
                                {
                                    avgTime = stopwatch.Elapsed.TotalMilliseconds;
                                }
                                else
                                {
                                    avgTime += (stopwatch.Elapsed.TotalMilliseconds - avgTime) / count;
                                    count++;
                                    max = stopwatch.Elapsed.TotalMilliseconds > max ? stopwatch.Elapsed.TotalMilliseconds : max;
                                }

                                if (!quiet)
                                {
                                    var firstLine = output.IndexOf("\n") != -1 ? output.Substring(0, output.IndexOf("\n")).Trim() : "";
                                    var threadId = "";
                                    if (threads > 1)
                                    {
                                        threadId = "Thread " + Thread.CurrentThread.ManagedThreadId + ". ";
                                    }
                                    Console.WriteLine(threadId + firstLine + ". " + string.Format("{0:N4}", stopwatch.Elapsed.TotalMilliseconds) + " ms. Avg: " + string.Format("{0:N4}", avgTime) + " ms. Max: " + string.Format("{0:N4}", max) + " ms.");
                                }

                                if (singleLineOutput) break;
                            }
                            else
                            {
                                Console.WriteLine(output);
                                Console.WriteLine("------------------------------\r\nTime taken: " + string.Format("{0:N4}", stopwatch.Elapsed.TotalMilliseconds) + " ms");
                                break;
                            }
                        }
                        catch (Exception e)
                        {
                            Console.WriteLine(e);
                            Console.WriteLine("------------------------------\r\nTime taken: " + string.Format("{0:N4}", stopwatch.Elapsed.TotalMilliseconds) + " ms");

                            if (!continuous) break;
                        }
                    }
                }).Start();
            }
        }

        private static string requestViaRawTcp(string host, int port, bool ssl, byte[] requestBytes)
        {
            var output = new StringBuilder();

            var c = new TcpClient(host, port);
            Stream tcpStream = c.GetStream();

            var s = tcpStream;
            if (ssl)
            {
                System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
                var sslStream = new SslStream(tcpStream, false, new RemoteCertificateValidationCallback((arg1, arg2, arg3, arg4) => true), null);
                //sslStream.AuthenticateAsClient(host);
                sslStream.AuthenticateAsClient(host, null, SslProtocols.Tls12, true);
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
