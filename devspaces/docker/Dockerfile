FROM ubuntu:18.04

#
# Install Raven env scripts dependencies
#
RUN apt-get update -y \
    && apt-get install --no-install-recommends -y \
        bzip2 \
        ca-certificates \
        curl \
        gnupg2 \
        jq \
        libunwind8 \
        lsb-release \
        nginx \
        sudo \
        vim \
        wget \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean -y
#
# Copy RavenDB *standard* install scripts and Run
#
COPY rdbscripts/* /opt/devspaces/installers/
RUN chmod +x /opt/devspaces/installers/*.sh \
    && /opt/devspaces/installers/install_build_prerequisites.sh \
    && /opt/devspaces/installers/install_runtime_prerequisites.sh \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean -y
#
# Copy assets
#
COPY assets /opt/devspaces/assets
RUN chmod +x /opt/devspaces/assets/scripts/* \
    && cp /opt/devspaces/assets/scripts/* /usr/local/bin/

#
# Install nodejs
#
RUN curl -sL https://deb.nodesource.com/setup_8.x | bash - \
    && apt-get -qq update \
    && apt-get -qqy install nodejs yarn \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean -y

#
# Create the non-root user
#
RUN adduser --disabled-password --gecos '' dev \
    && adduser dev sudo \
    && echo '%sudo ALL=(ALL) NOPASSWD:ALL' >> /etc/sudoers \
    && echo 'export LANG=en_US.UTF-8\nexport LC_ALL=en_US.UTF-8' >> ~/.bashrc \
    && chown -R dev:dev /tmp \
    && chown -R dev:dev /home/dev

USER dev

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

WORKDIR /data

EXPOSE 8080 38888 80

ENTRYPOINT tail -f /dev/null
