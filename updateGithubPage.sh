#! /usr/bin/env bash
[ "$#" -lt 1 ] && {
  echo 'Usage: createReleaseBundle.sh <version>'
  exit 2
}
ver="$1"  
[[ "$ver" =~ ^[0-9]+(\.[0-9]+)*$ ]] || {
  echo 'Illegal Version.'
  exit 2
}
dotnet publish client-browser/ -c Release -o page/ -p:Version="$ver" || echo "Failed to build $proj_type-$system"$'\n'
mv page/wwwroot page/html
