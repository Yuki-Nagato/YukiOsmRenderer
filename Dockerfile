# Runtime image of YukiOsmRenderer.
# The build context must contain the extracted Linux release bundles as bundle/amd64/ and bundle/arm64/, with their
# yukielevation moved to elevation/amd64/ and elevation/arm64/ (see .github/workflows/release.yml). Everything needed at
# runtime is inside the bundle, apart from glibc and OpenSSL, which the .NET runtime uses for cryptography.
#
# DEBIAN selects the base image: 12 (default) or 11, the glibc baseline of yukiosm. yukielevation needs glibc 2.35 and
# the libstdc++ of GCC 12, so the Debian 11 image goes without it.
#
#   docker run -p 8080:8080 --shm-size=1g -v /path/to/pbf:/input:ro -v /path/to/data:/data \
#       ghcr.io/yuki-nagato/yukiosmrenderer serve -i /input/region.osm.pbf --host 0.0.0.0
#
# /path/to/data is a host directory writable by UID 1000; it keeps the database between runs.

ARG DEBIAN=12

FROM scratch AS elevation-12
ARG TARGETARCH
COPY elevation/${TARGETARCH}/ /

FROM scratch AS elevation-11

FROM elevation-${DEBIAN} AS elevation

FROM debian:${DEBIAN}

ARG DEBIAN
ARG TARGETARCH

# Debian 11 comes with OpenSSL 1.1, Debian 12 without OpenSSL.
RUN if [ "$DEBIAN" != 11 ]; then \
        apt-get update \
        && apt-get install -y --no-install-recommends libssl3 \
        && rm -rf /var/lib/apt/lists/*; \
    fi

# PostgreSQL refuses to run as root.
RUN useradd --create-home --uid 1000 yukiosm \
    && mkdir /data \
    && chown yukiosm:yukiosm /data

COPY bundle/${TARGETARCH}/ /opt/yukiosm/
COPY --from=elevation / /opt/yukiosm/

ENV YUKIOSM_DATA_DIR=/data
USER yukiosm
EXPOSE 8080
ENTRYPOINT ["/opt/yukiosm/yukiosm"]
CMD ["--help"]
