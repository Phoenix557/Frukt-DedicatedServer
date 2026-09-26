#!/bin/sh
# Pterodactyl runs the egg's startup command through this, with {{VARIABLES}} filled in from the environment.
cd /home/container || exit 1

MODIFIED_STARTUP=$(printf '%s' "${STARTUP:-/opt/frukt/FruktServer}" | sed -e 's/{{/${/g' -e 's/}}/}/g')
echo ":/home/container$ ${MODIFIED_STARTUP}"
# exec, so a stop signal reaches the server instead of this shell.
eval "exec ${MODIFIED_STARTUP}"
