@echo off
cd ..\..\..\..\
docker build -t mare-synchronos-services:latest . -f server\Docker\build\Dockerfile-MareSynchronosServices --no-cache --pull --force-rm
cd Docker\build\windows-local