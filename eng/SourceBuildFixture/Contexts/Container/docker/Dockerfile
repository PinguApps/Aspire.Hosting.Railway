FROM docker.io/library/busybox@sha256:66a6306db78bf2dbf3487f293aa8d6990d8e506fdffab9cc43fe422becf886e4
ARG PROOF_VERSION
RUN test -n "$PROOF_VERSION" && mkdir /www && printf 'RAILWAY_CONTAINER_BUILD_%s' "$PROOF_VERSION" > /www/index.html
USER 65534
EXPOSE 8080
CMD ["httpd", "-f", "-p", "8080", "-h", "/www"]
