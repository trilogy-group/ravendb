# Development with Devspaces

### Devspaces

Manage your Devspaces https://www.devspaces.io/.

Read up-to-date documentation about cli installation and operation in https://www.devspaces.io/devspaces/help.

Here follows the main commands used in Devspaces cli.

|action   |Description                                                                                   |
|---------|----------------------------------------------------------------------------------------------|
|`devspaces --help`                    |Check the available command names.                               |
|`devspaces create [options]`          |Creates a DevSpace using your local DevSpaces configuration file |
|`devspaces start <devSpace>`          |Starts the DevSpace named \[devSpace\]                           |
|`devspaces bind <devSpace>`           |Syncs the DevSpace with the current directory                    |
|`devspaces info <devSpace> [options]` |Displays configuration info about the DevSpace.                  |

Use `devspaces --help` to know about updated commands.

All commands should be issued from **project directory**.


#### Before you begin

Initialize docker context scripts (for both Devspaces and docker compose): `devspaces/docker-cli.sh init`.

Fetch a **developer license** from RavenDB https://ravendb.net/buy. Save the json license file as `devspaces/docker/assets/license.json`. This file is in `.gitignore`.

#### Development flow

You should have Devspaces cli services started and logged to develop with Devspaces.

1 - Create Devspaces

```bash
cd devspaces/docker
devspaces create
cd ../../
```

2 - Start containers

```bash
devspaces start ravendbDev
```

3 - Start containers synchronization

```bash
devspaces bind ravendbDev
```

4 - Grab some container info

```bash
devspaces info ravendbDev
```

Retrieve published DNS and endpoints using this command

5 - Connect to development container

```bash
devspaces exec ravendbDev
```

6 - Build DB and install binaries

```bash
build_prep
./build.sh
```

7 - Run application

```bash
start_ravendb
```

Access application URLs:

Using information retrieved in step 4, access the following URL's:

* Application (bound to port 8080):
    * `http://ravendb.<devspaces-user>.devspaces.io:<published-ports>/`

8 - Run tests

```bash
install_license
dotnet test test/FastTests -c Release --filter "FullyQualifiedName!=FastTests.Issues.RavenDB_10493.CanTranslateDateTimeMinValueMaxValue"
```


### Docker Script Manager (CLI)

Currently, we have these command available to work using local docker compose.

```bash
devspaces/docker-cli.sh <command>
```

|action    |Description                                                               |
|----------|--------------------------------------------------------------------------|
|`build`   |Builds images                                                             |
|`deploy`  |Deploy Docker compose containers                                          |
|`undeploy`|Undeploy Docker compose containers                                        |
|`start`   |Starts Docker compose containers                                          |
|`stop`    |Stops Docker compose containers                                           |
|`exec`    |Get into the container                                                    |

#### Development flow

1 - Build and Run `docker-compose` locally.

```bash
devspaces/docker-cli.sh build
devspaces/docker-cli.sh deploy
devspaces/docker-cli.sh start
```

2 - Get into container

```bash
devspaces/docker-cli.sh exec
```

3 - Build DB and install binaries

```bash
build_prep
./build.sh
```

4 - Run application

```bash
start_ravendb
```

Access application URLs:

* Application (bound to port 8080):
    * http://localhost:8080

5 - Run tests

```bash
install_license
dotnet test test/FastTests -c Release --filter "FullyQualifiedName!=FastTests.Issues.RavenDB_10493.CanTranslateDateTimeMinValueMaxValue"
```
