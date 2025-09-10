@echo off
cd ..\..\..\..\
docker build -t sphene-services:latest . -f server\Docker\build\Dockerfile-SpheneServices --no-cache --pull --force-rm
cd server\Docker\build\windows-local