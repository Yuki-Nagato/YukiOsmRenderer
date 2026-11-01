# Runtime image of YukiOsmRenderer.
# The build context must contain the extracted Linux release bundles as bundle/amd64/ and bundle/arm64/
# (see .github/workflows/release.yml); everything needed at runtime is inside the bundle.
#
#   docker run -p 8080:8080 -v /path/to/pbf:/input:ro -v yukiosm-data:/data \
#       ghcr.io/yuki-nagato/yukiosmrenderer serve -i /input/region.osm.pbf --host 0.0.0.0

FROM debian:12-slim

ARG TARGETARCH

# PostgreSQL refuses to run as root.
RUN useradd --create-home --uid 1000 yukiosm \
    && mkdir /data \
    && chown yukiosm:yukiosm /data

COPY bundle/${TARGETARCH}/ /opt/yukiosm/

ENV YUKIOSM_DATA_DIR=/data
USER yukiosm
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["/opt/yukiosm/yukiosm"]
CMD ["--help"]
