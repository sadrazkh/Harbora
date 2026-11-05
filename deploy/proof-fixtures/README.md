# Proof fixtures

The apps `deploy/launch-proof.sh` launches on a throwaway host. Each one is what a customer's project
looks like when it reaches `harbora launch`, so none of them has a Dockerfile, and none has been bent to
make a check pass: if one of them is reported wrongly, the report is the bug.

Nothing here is shipped or built into the panel's image.
