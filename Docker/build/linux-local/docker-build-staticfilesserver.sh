#!/bin/sh
cd ../../../../
docker build -t sphene-staticfilesserver:latest . -f server/Docker/build/Dockerfile-SpheneStaticFilesServer --no-cache --pull --force-rm
cd server/Docker/build/linux-local