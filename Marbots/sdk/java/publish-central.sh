#!/usr/bin/env bash
# Publishes the Java SDK to Maven Central through the Sonatype Central Portal API.
# Requires: mvn, JDK (jar), gpg (signing key imported), curl, python3, and env vars
#   MAVEN_CENTRAL_USERNAME / MAVEN_CENTRAL_PASSWORD  (Central Portal user token)
#   GPG_PASSPHRASE                                   (passphrase of the signing key)
# Optional: PUBLISHING_TYPE=AUTOMATIC (default) | USER_MANAGED (validate only, publish from the portal UI)
set -euo pipefail
cd "$(dirname "$0")"

: "${MAVEN_CENTRAL_USERNAME:?missing}" "${MAVEN_CENTRAL_PASSWORD:?missing}" "${GPG_PASSPHRASE:?missing}"
PUBLISHING_TYPE="${PUBLISHING_TYPE:-AUTOMATIC}"

mvn -B -q package
GROUP=$(mvn -B -q help:evaluate -Dexpression=project.groupId -DforceStdout)
ARTIFACT=$(mvn -B -q help:evaluate -Dexpression=project.artifactId -DforceStdout)
VERSION=$(mvn -B -q help:evaluate -Dexpression=project.version -DforceStdout)
echo "Publishing $GROUP:$ARTIFACT:$VERSION ($PUBLISHING_TYPE)"

STAGE=target/central-bundle
DIR="$STAGE/${GROUP//.//}/$ARTIFACT/$VERSION"
rm -rf "$STAGE" && mkdir -p "$DIR"
cp "target/$ARTIFACT-$VERSION.jar" "target/$ARTIFACT-$VERSION-sources.jar" "target/$ARTIFACT-$VERSION-javadoc.jar" "$DIR/"
cp pom.xml "$DIR/$ARTIFACT-$VERSION.pom"
(
  cd "$DIR"
  for f in *.jar *.pom; do
    gpg --batch --yes --pinentry-mode loopback --passphrase "$GPG_PASSPHRASE" --armor --detach-sign "$f"
    md5sum "$f" | cut -d' ' -f1 > "$f.md5"
    sha1sum "$f" | cut -d' ' -f1 > "$f.sha1"
  done
)
(cd "$STAGE" && jar -cMf ../central-bundle.zip .)   # JDK jar tool: no zip dependency

AUTH=$(printf '%s:%s' "$MAVEN_CENTRAL_USERNAME" "$MAVEN_CENTRAL_PASSWORD" | base64 | tr -d '\n')
ID=$(curl -sf -H "Authorization: Bearer $AUTH" -F "bundle=@target/central-bundle.zip" \
  "https://central.sonatype.com/api/v1/publisher/upload?publishingType=$PUBLISHING_TYPE&name=$ARTIFACT-$VERSION")
echo "Deployment: $ID"

for _ in $(seq 1 120); do
  STATUS=$(curl -sf -X POST -H "Authorization: Bearer $AUTH" "https://central.sonatype.com/api/v1/publisher/status?id=$ID")
  STATE=$(echo "$STATUS" | python3 -c "import sys,json;print(json.load(sys.stdin)['deploymentState'])")
  echo "State: $STATE"
  case "$STATE" in
    PUBLISHED|VALIDATED) echo "$STATUS"; exit 0 ;;
    FAILED) echo "$STATUS"; exit 1 ;;
  esac
  sleep 10
done
echo "Timed out waiting for Central (deployment $ID)"; exit 1
