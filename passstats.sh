#!/bin/bash

# Read sim output from stdin and count end: messages, and pass messages by personality split into corner/straight plus grass count

# Count end: messages
echo "=== End reasons ==="
grep -c 'end:' /dev/stdin

# Count pass messages by personality, corner/straight and grass
echo -e "\n=== Pass stats by personality ==="
# Process pass lines
grep 'pass ' /dev/stdin | \
sed 's/pass \([A-Za-z]*\) \([a-z]*\)\(.*\)/\1 \2\3/' | \
sed 's/ \(.*\)grass/\1 grass/' | \
sed 's/ \(.*\)corner/\1 corner/' | \
sed 's/ \(.*\)straight/\1 straight/' | \
sort | uniq -c