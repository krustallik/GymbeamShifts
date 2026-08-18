FROM alpine:3.20

ARG FIXTURE_VERSION
RUN apk add --no-cache curl
COPY health-server.sh /usr/local/bin/health-server
RUN chmod 755 /usr/local/bin/health-server
ENV FIXTURE_VERSION=${FIXTURE_VERSION}
HEALTHCHECK --interval=1s --timeout=1s --retries=20 CMD curl --fail --silent http://127.0.0.1:8080/healthz >/dev/null || exit 1
CMD ["/usr/local/bin/health-server"]
