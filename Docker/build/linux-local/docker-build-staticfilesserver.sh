#!/bin/sh
cd ../../../../
docker build -t mare-synchronos-staticfilesserver:latest . -f server/Docker/build/Dockerfile-MareSynchronosStaticFilesServer --no-cache --pull --force-rm
cd Docker/build/linux-local