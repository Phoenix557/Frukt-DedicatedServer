# Image for the FRUKT dedicated server on Pterodactyl. FruktServer is one self-contained file (build-image.ps1 publishes it
# into docker/bin), so the image needs no .NET, no Wine and no game: players' games run the world, the server passes packets.
# The egg installs FruktServer into /home/container; the image keeps a copy so a restart can update it.
FROM debian:bookworm-slim
LABEL org.opencontainers.image.source=https://github.com/Phoenix557/Frukt-DedicatedServer
LABEL org.opencontainers.image.description="Dedicated server for the FRUKT Multiplayer mod"

RUN apt-get update \
    && apt-get install -y --no-install-recommends tini tzdata \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -m -d /home/container -s /bin/sh container

COPY --chmod=755 bin/FruktServer /opt/frukt/FruktServer
COPY --chmod=755 entrypoint.sh /entrypoint.sh

USER container
ENV USER=container HOME=/home/container
WORKDIR /home/container
EXPOSE 27777/udp
ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["/bin/sh", "/entrypoint.sh"]
