# Runtime image of YukiOsmRenderer.
# The build context must contain the extracted Linux release bundles as bundle/amd64/ and bundle/arm64/
# (see .github/workflows/release.yml); everything needed at runtime is inside the bundle, apart from glibc and
# OpenSSL, which the .NET runtime uses for cryptography. Debian 11 matches the glibc baseline the bundle is built for.
#
#   docker run -p 8080:8080 --shm-size=1g -v /path/to/pbf:/input:ro -v /path/to/data:/data \
#       ghcr.io/yuki-nagato/yukiosmrenderer serve -i /input/region.osm.pbf --host 0.0.0.0
#
# /path/to/data is a host directory writable by UID 1000; it keeps the database between runs.

FROM debian:11

ARG TARGETARCH

# PostgreSQL refuses to run as root.
RUN useradd --create-home --uid 1000 yukiosm \
    && mkdir /data \
    && chown yukiosm:yukiosm /data

COPY bundle/${TARGETARCH}/ /opt/yukiosm/

ENV YUKIOSM_DATA_DIR=/data
USER yukiosm
EXPOSE 8080
ENTRYPOINT ["/opt/yukiosm/yukiosm"]
CMD ["--help"]
