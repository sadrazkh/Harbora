# Publishes a message to the broker it was given and reads it back, by the two names a project asks
# under: the one an attachment sets, and the one this project's own example file uses.
import json
import os
from http.server import BaseHTTPRequestHandler, HTTPServer

import pika


def round_trip(url):
    if not url:
        return {"ok": False, "error": "the variable is empty"}

    try:
        parameters = pika.URLParameters(url)
        parameters.socket_timeout = 5
        # A broker short of memory stops taking messages without refusing them, and a publisher waits.
        parameters.blocked_connection_timeout = 10
        connection = pika.BlockingConnection(parameters)
        try:
            channel = connection.channel()
            channel.confirm_delivery()
            queue = channel.queue_declare(queue="", exclusive=True).method.queue
            channel.basic_publish(exchange="", routing_key=queue, body=b"hello", mandatory=True)
            method, _, body = channel.basic_get(queue=queue, auto_ack=True)
            return {"ok": method is not None and body == b"hello", "echo": (body or b"").decode()}
        finally:
            connection.close()
    except Exception as error:  # what went wrong is the answer
        return {"ok": False, "error": f"{type(error).__name__}: {error}"[:200]}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        body = json.dumps({
            "viaAttachName": round_trip(os.environ.get("RABBITMQ_URL")),
            "viaExampleName": round_trip(os.environ.get("CLOUDAMQP_URL")),
        }).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    port = int(os.environ.get("PORT", "8000"))
    print(f"python-rabbitmq is listening on port {port}", flush=True)
    HTTPServer(("0.0.0.0", port), Handler).serve_forever()
