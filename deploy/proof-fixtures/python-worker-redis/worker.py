# A loop and nothing else: it listens on no port, so it has no address to probe and no domain to give.
import os
import time

import redis

client = redis.Redis.from_url(os.environ["REDIS_URL"], socket_connect_timeout=5)

while True:
    try:
        print(f"redis answered: ping={client.ping()} ticks={client.incr('proof:ticks')}", flush=True)
    except redis.RedisError as error:
        print(f"redis did not answer: {error}", flush=True)
    time.sleep(5)
