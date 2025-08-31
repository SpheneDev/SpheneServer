#!/bin/sh
cd ../../../
docker build -t mare-synchronos-server:latest . -f Docker/build/Dockerfile-MareSynchronosServer --no-cache --pull --force-rm
cd Docker/build/linux-local