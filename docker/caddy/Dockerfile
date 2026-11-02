# Caddy serving the built frontend. Vite puts the Clerk publishable key into the bundle at build time.
FROM node:24-alpine AS build
ARG CLERK_PUBLISHABLE_KEY
ENV VITE_CLERK_PUBLISHABLE_KEY=$CLERK_PUBLISHABLE_KEY
WORKDIR /frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM caddy:2.11.7-alpine
COPY docker/caddy/Caddyfile /etc/caddy/Caddyfile
COPY --from=build /frontend/dist /srv
