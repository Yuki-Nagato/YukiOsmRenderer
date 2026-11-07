# Runtime image of yukielevation, the elevation service, without the rest of YukiOsmRenderer.
# The build context must contain, for each architecture, yukielevation, LICENSE, THIRD-PARTY-NOTICES.md and the
# licenses/ of runtime/licenses/yukielevation from the Linux release bundles as bundle/amd64/ and bundle/arm64/ (see
# .github/workflows/release.yml). Its GDAL needs glibc >= 2.35 and the libstdc++ of GCC >= 12, which Debian 12 has.
#
#   docker run -p 8100:8100 -v /path/to/gebco:/data/gebco:ro -v /path/to/glo30:/data/glo30:ro \
#       ghcr.io/yuki-nagato/yukielevation --gebco /data/gebco --glo30 /data/glo30

FROM debian:12

ARG TARGETARCH

COPY bundle/${TARGETARCH}/ /opt/yukielevation/

RUN useradd --create-home --uid 1000 yukielevation
USER yukielevation

# The executable extracts its GDAL libraries and data on the first start, into a directory only its user may read;
# this does it once in the image.
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/home/yukielevation/.net
RUN /opt/yukielevation/yukielevation --help > /dev/null

EXPOSE 8100
ENTRYPOINT ["/opt/yukielevation/yukielevation", "--host", "0.0.0.0"]
CMD ["--gebco", "/data/gebco"]
