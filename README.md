# rawrequestgen

Takes an input file with an HTTP requests and sends it almost raw on a socket.

Download: https://github.com/allanrbo/rawrequestgen/releases/download/untagged-6bcf5c48e4d89534af97/rawrequestgen.exe

```
Usage: rawrequestgen.exe example.com request1.txt [--print-request-headers] [--ssl] [-c] [-s] [-t 5] [-q]
-s   Single line output
-c   Continuous
-t   Concurrent thread count
-q   Quiet
request1.txt may contain {{bodylength}} which will get replaced with actual body length
```

Example input file:
```
POST /?a=1 HTTP/1.1
Content-Type: multipart/form-data; boundary=------------------------1aa6ce6559102
Accept: */*
Host: example.net
Max-Forwards: 10
User-Agent: myuseragent
Cookie: test123=123; test456=123
Content-Length: {{bodylength}}

--------------------------1aa6ce6559102
content-disposition: form-data; name="b"

Hello world.
--------------------------1aa6ce6559102--
```
