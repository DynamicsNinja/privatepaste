# The product homepage (privatepaste.ficdev.xyz). Build from the repository root so the page reuses the logo, the
# web app's Sora font and the server version from the Dokploy compose file instead of keeping copies:
#   docker build -f site/Dockerfile .
FROM nginx:1.29-alpine

# A template: at start the image substitutes ${NGINX_LOCAL_RESOLVERS} (and only defined variables, so nginx's own
# $variables are left alone) and writes it to conf.d/default.conf.
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
COPY site/nginx.conf /etc/nginx/templates/default.conf.template
COPY site/index.html site/privacy.html site/style.css site/site.js site/favicon.ico site/apple-touch-icon.png site/robots.txt site/sitemap.xml /usr/share/nginx/html/
COPY site/images/ /usr/share/nginx/html/images/
COPY branding/logo.svg /usr/share/nginx/html/logo.svg
COPY src/PrivatePaste.Server/wwwroot/fonts/sora-latin.woff2 src/PrivatePaste.Server/wwwroot/fonts/sora-latin-ext.woff2 /usr/share/nginx/html/fonts/
COPY deploy/dokploy/privatepaste/docker-compose.yml /tmp/server-compose.yml

# The sign-up form's handler (POST /notify) runs in nginx's njs module, which this image already ships; it is loaded
# in the main nginx.conf because load_module can't go in conf.d. Addresses go to /data/signups, a volume the owner
# mounts in Dokploy (site/README.md); a start script (run as root, before nginx) hands it to the nginx user the
# workers run as. No VOLUME here: a forgotten mount should be noticed, not saved to a nameless volume.
COPY site/njs/notify.js /etc/nginx/njs/notify.js
COPY site/signups-dir.sh /docker-entrypoint.d/40-signups-dir.sh
RUN test -f /etc/nginx/modules/ngx_http_js_module.so \
 && sed -i '1i load_module modules/ngx_http_js_module.so;' /etc/nginx/nginx.conf \
 && chmod +x /docker-entrypoint.d/40-signups-dir.sh

# The page shows the current server version (the trust line and the image tag in the compose examples). It is the
# tag in the Dokploy compose file, which every server release bumps, so the page never needs editing for it.
RUN v=$(sed -n 's#^ *image: ghcr\.io/dynamicsninja/privatepaste:\([0-9][^ ]*\) *$#\1#p' /tmp/server-compose.yml) \
 && test -n "$v" \
 && sed -i "s#__SERVER_VERSION__#$v#g" /usr/share/nginx/html/index.html \
 && ! grep -q __SERVER_VERSION__ /usr/share/nginx/html/index.html \
 && echo "server version $v" \
 && rm /tmp/server-compose.yml

# Browsers and Cloudflare cache CSS, JS and images for an hour (nginx.conf), so stamp each reference with the
# file's content hash, on every page: a changed file gets a new URL and a deploy shows up at once. Only site-relative
# references are stamped (a quote, space or comma before and after, as in href, src and srcset); the absolute
# og:image and twitter:image URLs are left alone. A page with nothing stamped fails the build.
RUN cd /usr/share/nginx/html \
 && for f in style.css site.js logo.svg favicon.ico apple-touch-icon.png images/*; do \
        v=$(md5sum "$f" | cut -c1-10); \
        sed -i "s#\([\" ,]\)/$f\([\" ,]\)#\1/$f?v=$v\2#g" index.html privacy.html; \
    done \
 && for page in index.html privacy.html; do echo "$page: $(grep -o '?v=' $page | wc -l) stamped"; grep -q '?v=' $page; done

# Check the configuration and compile njs/notify.js now, so a mistake fails the build instead of the deploy. The
# entrypoint writes conf.d/default.conf again at start, with the container's real resolvers.
RUN NGINX_LOCAL_RESOLVERS=127.0.0.11 envsubst '${NGINX_LOCAL_RESOLVERS}' \
        < /etc/nginx/templates/default.conf.template > /etc/nginx/conf.d/default.conf \
 && nginx -t

EXPOSE 80
