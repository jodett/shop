ARG PHP_VERSION=8.4
ARG SHOPWARE_VERSION=6.7.8.1

FROM php:${PHP_VERSION}-cli AS builder

ENV COMPOSER_MEMORY_LIMIT=-1
ENV PHP_MEMORY_LIMIT=1G

RUN apt-get update && apt-get install -y \
    git unzip curl \
    libzip-dev \
    libpng-dev \
    libjpeg62-turbo-dev \
    libfreetype6-dev \
    libicu-dev \
    libonig-dev \
    libxml2-dev \
    libcurl4-openssl-dev \
    libbz2-dev \
 && docker-php-ext-configure gd --with-freetype --with-jpeg \
 && docker-php-ext-install gd intl pdo_mysql zip bz2 mbstring xml

RUN echo "memory_limit=${PHP_MEMORY_LIMIT}" > /usr/local/etc/php/conf.d/memory-limit.ini

RUN curl -sS https://getcomposer.org/installer | php -- --install-dir=/usr/local/bin --filename=composer

WORKDIR /build

RUN composer create-project shopware/production:${SHOPWARE_VERSION} . --no-interaction

RUN composer require shopware/docker --no-interaction

RUN composer install --no-dev --optimize-autoloader --no-interaction

FROM ghcr.io/shopware/docker-base:${PHP_VERSION}-caddy AS runtime

WORKDIR /var/www/html

USER root

COPY --from=builder --chown=82:82 /build /var/www/html

RUN mkdir -p \
    /var/www/html/files \
    /var/www/html/public/theme \
    /var/www/html/public/media \
    /var/www/html/public/thumbnail \
    /var/www/html/public/sitemap \
 && chown -R 82:82 /var/www/html

USER www-data