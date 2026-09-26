#!/bin/sh
# Pterodactyl runs the egg's startup command through this, with {{VARIABLES}} filled in from the environment.
cd /home/container || exit 1

# FruktServer updates itself from GitHub releases; the image's copy is only for a server folder that has none.
if [ ! -f FruktServer ] && [ -f /opt/frukt/FruktServer ]; then
    cp /opt/frukt/FruktServer FruktServer && chmod +x FruktServer
fi

MODIFIED_STARTUP=$(printf '%s' "${STARTUP:-./FruktServer}" | sed -e 's/{{/${/g' -e 's/}}/}/g')
echo ":/home/container$ ${MODIFIED_STARTUP}"
# exec, so a stop signal reaches the server instead of this shell.
eval "exec ${MODIFIED_STARTUP}"
