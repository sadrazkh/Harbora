# Runs inside the image's `pip install` step, so what it writes is what that step could see.
import os

from setuptools import setup

with open("/app/build-mark.txt", "w") as mark:
    mark.write(os.environ.get("BUILD_MARK", ""))

setup(name="probe", version="1.0", py_modules=[])
