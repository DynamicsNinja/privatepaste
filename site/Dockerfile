# The product homepage (privatepaste.ficdev.xyz). Build from the repository root so the page reuses the logo, the
# web app's Sora font and the server version from the Dokploy compose file instead of keeping copies:
#   docker build -f site/Dockerfile .
FROM nginx:1.29-alpine

# A template: at start the image substitutes ${NGINX_LOCAL_RESOLVERS} (and only defined variables, so nginx's own
# $variables are left alone) and writes it to conf.d/default.conf.
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
COPY site/nginx.conf /etc/nginx/templates/default.conf.template
COPY site/index.html site/style.css site/site.js site/favicon.ico site/apple-touch-icon.png site/robots.txt site/sitemap.xml /usr/share/nginx/html/
COPY site/images/ /usr/share/nginx/html/images/
COPY branding/logo.svg /usr/share/nginx/html/logo.svg
COPY src/PrivatePaste.Server/wwwroot/fonts/sora-latin.woff2 src/PrivatePaste.Server/wwwroot/fonts/sora-latin-ext.woff2 /usr/share/nginx/html/fonts/
COPY deploy/dokploy/privatepaste/docker-compose.yml /tmp/server-compose.yml

# The page shows the current server version (the trust line and the image tag in the compose examples). It is the
# tag in the Dokploy compose file, which every server release bumps, so the page never needs editing for it.
RUN v=$(sed -n 's#^ *image: ghcr\.io/dynamicsninja/privatepaste:\([0-9][^ ]*\) *$#\1#p' /tmp/server-compose.yml) \
 && test -n "$v" \
 && sed -i "s#__SERVER_VERSION__#$v#g" /usr/share/nginx/html/index.html \
 && ! grep -q __SERVER_VERSION__ /usr/share/nginx/html/index.html \
 && echo "server version $v" \
 && rm /tmp/server-compose.yml

# Browsers and Cloudflare cache CSS, JS and images for an hour (nginx.conf), so stamp each reference with the
# file's content hash: a changed file gets a new URL and a deploy shows up at once. Only site-relative references
# are stamped (a quote, space or comma before and after, as in href, src and srcset); the absolute og:image and
# twitter:image URLs are left alone.
RUN cd /usr/share/nginx/html \
 && for f in style.css site.js logo.svg favicon.ico apple-touch-icon.png images/*; do \
        v=$(md5sum "$f" | cut -c1-10); \
        sed -i "s#\([\" ,]\)/$f\([\" ,]\)#\1/$f?v=$v\2#g" index.html; \
    done \
 && grep -o '?v=' index.html | wc -l

EXPOSE 80
