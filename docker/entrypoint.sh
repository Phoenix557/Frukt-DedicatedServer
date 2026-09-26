#!/bin/sh
# Pterodactyl runs the egg's startup command through this, with {{VARIABLES}} filled in from the environment.
cd /home/container || exit 1

# The image carries the newest FruktServer; copy it over the installed one unless AUTO_UPDATE is 0.
if [ "${AUTO_UPDATE:-1}" != "0" ] && [ -f /opt/frukt/FruktServer ]; then
    if [ ! -f FruktServer ] || ! cmp -s /opt/frukt/FruktServer FruktServer; then
        cp -f /opt/frukt/FruktServer FruktServer && chmod +x FruktServer && echo "Updated FruktServer to the image's version."
    fi
fi

MODIFIED_STARTUP=$(printf '%s' "${STARTUP:-./FruktServer}" | sed -e 's/{{/${/g' -e 's/}}/}/g')
echo ":/home/container$ ${MODIFIED_STARTUP}"
# exec, so a stop signal reaches the server instead of this shell.
eval "exec ${MODIFIED_STARTUP}"
