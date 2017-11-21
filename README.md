# rawrequestgen

Download: https://github.com/allanrbo/rawrequestgen/releases/download/1/rawrequestgen.exe

Takes an input file and sends it almost raw on a socket.

Usage: rawrequestgen.exe example.com request1.txt [--print-request] [--ssl]

request1.txt may contain {{bodylength}} which will get replaced with actual body length.

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
