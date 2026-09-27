#!/usr/bin/env bash
cd "$(dirname "$(readlink -f "$0")")/server" && exec ./AssettoServer "$@"
