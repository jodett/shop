# shopware-k3s Helm chart

Dieses Chart ist bewusst **produktionsorientiert**, aber **kein offizielles Shopware-Chart**. Es setzt auf:

- eigenes Shopware-Runtime-Image
- externes MariaDB
- externes Redis
- Longhorn-PVC für Medien
- Traefik Ingress
- optionalen Worker und Scheduled Tasks

## Warum kein offizielles Shopware-Chart?

Die offiziellen Shopware-Helm-Charts und der Operator sind aktuell noch als experimentell gekennzeichnet. Deshalb ist dieses Chart als pragmatisches Grundgerüst für k3s gedacht.

## Voraussetzungen

- k3s mit Traefik
- cert-manager
- Longhorn
- MariaDB 10.11+
- Redis 7+
- eigenes Shopware-Image

## Eigenes Image bauen

Shopware stellt ein Runtime-Image bereit, das **Shopware selbst nicht enthält**. Du baust daher ein eigenes Image, in das dein Shopware-Projekt kopiert wird.

Beispiel:

```dockerfile
FROM ghcr.io/shopware/docker-base:8.2-caddy AS base
WORKDIR /var/www/html
COPY . /var/www/html
RUN composer install --no-dev --optimize-autoloader \
 && php bin/console bundle:dump \
 && php bin/console assets:install
CMD ["php-fpm", "-F"]
```

## Installation

```bash
helm upgrade --install shopware ./shopware-k3s-chart -n shop --create-namespace \
  -f values.yaml
```

## Wichtige Werte

- `image.repository`, `image.tag`: dein Runtime-Image
- `shopware.appUrl`: öffentliche URL
- `shopware.appSecret`, `shopware.instanceId`: echte Werte setzen
- `mariadb.*`: externer DB-Zugang
- `redis.url`: externer Redis-Zugang
- `persistence.media.*`: Longhorn-PVC
- `ingress.*`: Host/TLS/Issuer

## Nächste sinnvolle Schritte

1. `startupJob.enabled` nur bewusst und nur für Erstinitialisierung aktivieren.
2. Media-PVC später auf RWX umstellen, falls du mehrere App-Replikas willst.
3. Backups separat ergänzen.
4. eBay-Sync später als separates Chart oder Deployment hinzufügen.
