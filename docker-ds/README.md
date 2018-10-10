# RavenDB Dockerization


## Build the Docker image

The Dockerfile is created out of `mcr.microsoft.com/powershell` image. Git, .NET Core 2.1, npm and other basic developer tools are available in the image. 

# Quick Start Using Docker-Compose

- Switch to the `{ravendb-project-root-folder}/docker-ds` or extract the contents of the RavenDB artifacts to the  `{ravendb-project-root-folder}/docker-ds` directory.
- Open a terminal session to that folder.  Run `./docker-cli start` : Build and run the containers
- Run `./docker-cli exec`
- At this point you must be inside the docker container, in the root folder of the project in the container (i.e.: `/data`). From there, you can build the repository as usual:
    - `./build.sh` to build RavenDB.  This will generates build contents under  `{ravendb-project-root-folder}/artifacts`
    - See the section *Using RavenDB* for details on how to use RavenDB Docker container for running the RavenDB Server
- When you finish working with the container, type `exit`
- Run `./docker-cli stop` to stop the service or `./docker-cli down` to stop and remove the 

# Work with RavenDB Development Docker using Docker-Compose

Unzip the RavenDB Dockerization artifacts to the `{ravendb-project-root-folder}/docker-ds/`.
Change direcotry to the  `{ravendb-project-root-folder}/docker-ds/`

## Build and Run the container

In `{ravendb-project-root-folder}/docker-ds/` folder, run:

```
./docker-cli start
```
This command will use the Dockerfile to create a Docker image and then start the container in detach mode.

### Build the container

In `{ravendb-project-root-folder}/docker-ds/` folder, run:

```
./docker-cli build
```
This command will use the Dockerfile and create a Docker image complete with all the tools to build and run RavenDB.

### Run the container

In `{ravendb-project-root-folder}/docker-ds/` folder, run:

```
./docker-cli up
```
This command will create a running container in detached mode called `ravendb-dev`.
You can check the containers running with `docker ps`

## Get a container session

Run:

```
./docker-cli exec
```

This will launch an interactive `bash` session into the `ravendb-dev` container.

## Stop/Remove the container

In `{ravendb-project-root-folder}/docker-ds/` folder, run:

```
./docker-cli stop
```
This command will create a stop the container.

```
./docker-cli down
```

This command will stop and remove the container.

## docker-compose.yml

The docker-compose.yml file contains a single service: `ravendb-dev`.  We will use this service to build the RavenDB sources from our local environment, so we mount the RavenDB root project dir to the a `/data` folder:

```
    volumes:
      - ../.:/data:Z
```

## Requirements
The container was tested successfully on:
- Docker 17.05 and up
- docker-compose 1.8 and up


# Using RavenDB Server

## Dependencies
The RavenDB uses a configuration file name `settings.json` which located in the same location as the RavenDB executable `Raven.Server`.  When launching the RavenDB server there are command-line arguments that can be used.  One of these is the `-c` option which allows the user to specify the `settings.json` that will be use to launch the RavenDB server.
For development docker container, we will use our own `settings.json` to allow remote access to the RavenDB server from an external client via HTTP.  A custom `settings.json` is also provided as part of the Dockerized development artifacts so that the original repos `settings.json` is not affected if we need to do any changes.

## Running RavenDB Server with Dockerized Development Container

To launch the built RavenDB Server with the custom development environment `settings.json`, simply run 
```
/data/docker-ds/start_ravendb
```
This will launch RavenDB in interactive mode and present the user with a command-line prompt.  The RavenDB administration UI (named Studio) is also available via locally on port 8080 and remotely via the exposed port (You can see the exposed port with the result from `docker ps | grep ravendb-dev`).

If you want to launch the RavenDB but without the interactive command mode then append an `-n` (for non-interactive) at the end of the command-line that launch the RavenDB server. 
