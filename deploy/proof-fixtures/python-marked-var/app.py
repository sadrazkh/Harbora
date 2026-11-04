import json
import os
from http.server import BaseHTTPRequestHandler, HTTPServer


class Answer(BaseHTTPRequestHandler):
    def do_GET(self):
        with open("/app/build-mark.txt") as mark:
            body = json.dumps({"builtWith": mark.read()}).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(body)


HTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8000"))), Answer).serve_forever()
