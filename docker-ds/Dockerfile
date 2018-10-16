FROM mcr.microsoft.com/powershell

RUN apt-get update -y \
    && apt-get install --no-install-recommends curl bzip2 libunwind8 ca-certificates lsb-release sudo wget jq -y \
    && mkdir -p /installers \
    && cd /installers \
    && wget http://mirrors.kernel.org/ubuntu/pool/main/i/icu/libicu52_52.1-3ubuntu0.8_amd64.deb \
    && dpkg -i libicu52*.deb \
    && rm -f libicu52_52.1-3ubuntu0.8_amd64.deb \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get clean -y

COPY scripts/* /installers/

RUN chmod +x /installers/*.sh \
    && /installers/install_build_prerequisites.sh \
    && /installers/install_runtime_prerequisites.sh

# Create the non-root user
RUN mkdir -p /data \
    && adduser --disabled-password --gecos '' dev \
    && adduser dev sudo \
    && echo '%sudo ALL=(ALL) NOPASSWD:ALL' >> /etc/sudoers \
    && chown -R dev /data
    
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

USER dev

RUN echo 'export LANG=en_US.UTF-8\nexport LC_ALL=en_US.UTF-8' >> ~/.bashrc

WORKDIR /data

EXPOSE 8080

CMD tail -f /dev/null
