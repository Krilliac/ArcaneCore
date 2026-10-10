#!/bin/bash
# First start of the database volume only (MariaDB docker-entrypoint-initdb.d): the three ArcaneCore schemas, owned by
# the application user. The daemons create and upgrade the tables themselves (Database:Upgrade:Policy).
set -euo pipefail
mariadb -uroot -p"${MARIADB_ROOT_PASSWORD}" <<SQL
CREATE DATABASE IF NOT EXISTS arcanecore_auth CHARACTER SET utf8mb4;
CREATE DATABASE IF NOT EXISTS arcanecore_characters CHARACTER SET utf8mb4;
CREATE DATABASE IF NOT EXISTS arcanecore_world CHARACTER SET utf8mb4;
GRANT ALL PRIVILEGES ON arcanecore_auth.* TO '${MARIADB_USER}'@'%';
GRANT ALL PRIVILEGES ON arcanecore_characters.* TO '${MARIADB_USER}'@'%';
GRANT ALL PRIVILEGES ON arcanecore_world.* TO '${MARIADB_USER}'@'%';
FLUSH PRIVILEGES;
SQL
