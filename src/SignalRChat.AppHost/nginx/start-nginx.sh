#!/bin/sh
set -eu

config_file=/etc/nginx/conf.d/default.conf

{
    printf '%s\n' \
        'map $http_connection $connection_upgrade {' \
        '    "~*Upgrade" $http_connection;' \
        '    default keep-alive;' \
        '}' \
        '' \
        'map $cookie_signalr_affinity $signalr_affinity_key {' \
        '    "" $remote_addr;' \
        '    default $cookie_signalr_affinity;' \
        '}' \
        '' \
        'upstream signalr_backend {' \
        '    hash $signalr_affinity_key consistent;'

    index=1
    while [ "$index" -le "$API_COUNT" ]; do
        eval "hostport=\${API_${index}_HOSTPORT:-}"

        if [ -z "$hostport" ]; then
            echo "Missing API_${index}_HOSTPORT" >&2
            exit 1
        fi

        printf '    server %s max_fails=3 fail_timeout=10s;\n' "$hostport"
        index=$((index + 1))
    done

    printf '%s\n' \
        '}' \
        '' \
        'upstream web_backend {' \
        "    server ${WEB_HOSTPORT} max_fails=3 fail_timeout=10s;" \
        '}' \
        '' \
        'server {' \
        '    listen 80;' \
        '    server_name _;' \
        '' \
        '    location = /nginx-health {' \
        '        access_log off;' \
        '        default_type application/json;' \
        '        return 200 '\''{"status":"healthy"}'\'';' \
        '    }' \
        '' \
        '    location /chatHub {' \
        '        proxy_pass http://signalr_backend;' \
        '        proxy_http_version 1.1;' \
        '        proxy_set_header Upgrade $http_upgrade;' \
        '        proxy_set_header Connection $connection_upgrade;' \
        '        proxy_cache off;' \
        '        proxy_buffering off;' \
        '        proxy_read_timeout 100s;' \
        '        proxy_set_header Host $host;' \
        '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' \
        '        proxy_set_header X-Forwarded-Proto $scheme;' \
        '    }' \
        '' \
        '    location ~ ^/(register|login|logout)(/|$) {' \
        '        proxy_pass http://signalr_backend;' \
        '        proxy_http_version 1.1;' \
        '        proxy_set_header Host $host;' \
        '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' \
        '        proxy_set_header X-Forwarded-Proto $scheme;' \
        '    }' \
        '' \
        '    location ^~ /account/ {' \
        '        proxy_pass http://signalr_backend;' \
        '        proxy_http_version 1.1;' \
        '        proxy_set_header Host $host;' \
        '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' \
        '        proxy_set_header X-Forwarded-Proto $scheme;' \
        '    }' \
        '' \
        '    location ~ ^/conversations(/|$) {' \
        '        proxy_pass http://signalr_backend;' \
        '        proxy_http_version 1.1;' \
        '        proxy_set_header Host $host;' \
        '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' \
        '        proxy_set_header X-Forwarded-Proto $scheme;' \
        '    }' \
        '' \
        '    location / {' \
        '        proxy_pass http://web_backend;' \
        '        proxy_http_version 1.1;' \
        '        proxy_set_header Host $host;' \
        '        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;' \
        '        proxy_set_header X-Forwarded-Proto $scheme;' \
        '    }' \
        '}'
} > "$config_file"

nginx -t
exec nginx -g 'daemon off;'
