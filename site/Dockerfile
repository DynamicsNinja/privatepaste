# The product homepage (privatepaste.ficdev.xyz). Build from the repository root so the page reuses the README
# hero images, the logo and the web app's Sora font instead of keeping copies:
#   docker build -f site/Dockerfile .
FROM nginx:1.29-alpine

# A template: at start the image substitutes ${NGINX_LOCAL_RESOLVERS} (and only defined variables, so nginx's own
# $variables are left alone) and writes it to conf.d/default.conf.
ENV NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1
COPY site/nginx.conf /etc/nginx/templates/default.conf.template
COPY site/index.html site/style.css site/site.js /usr/share/nginx/html/
COPY branding/logo.svg /usr/share/nginx/html/logo.svg
COPY docs/images/hero-dark.png docs/images/hero-light.png /usr/share/nginx/html/images/
COPY src/PrivatePaste.Server/wwwroot/fonts/sora-latin.woff2 src/PrivatePaste.Server/wwwroot/fonts/sora-latin-ext.woff2 /usr/share/nginx/html/fonts/

EXPOSE 80
