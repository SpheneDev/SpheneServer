#!/bin/sh
cd ../../../../
docker build -t sphene-authservice:latest . -f server/Docker/build/Dockerfile-SpheneAuthService --no-cache --pull --force-rm
cd server/Docker/build/linux-local