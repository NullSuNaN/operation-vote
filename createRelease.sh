 #! /usr/bin/env bash
unset ver filter dict tgList tgDict _tgList thread_limit i system proj_type dict_proj output err failed
[ "$#" -lt 1 ] && {
  echo 'Usage: createReleaseBundle.sh <version> [<filter:regex>:.*]'
  exit 2
}
ver="$1"  
filter="${2-.*}"
[[ "$ver" =~ ^[0-9]+(\.[0-9]+)*$ ]] || {
  echo 'Illegal Version.'
  exit 2
}
cd "${0%/*}"
dict="release/$ver"
thread_limit=4
mkdir -p "$dict"
[ "${dict:0:1}" == 'r' ] &&
  rm -rf "$dict"/* "$dict"/.* 2>/dev/null

mkfifo "$dict/lock.fifo"
exec 3>&-
exec 3<> "$dict/lock.fifo"
rm "$dict/lock.fifo"
mkfifo "$dict/exclusiveLock.fifo"
exec 4>&-
exec 4<> "$dict/exclusiveLock.fifo"
rm "$dict/exclusiveLock.fifo"
mkfifo "$dict/error.fifo"
exec 5>&-
exec 5<> "$dict/error.fifo"
rm "$dict/error.fifo"
mkfifo "$dict/clientBrowserLock.fifo"
exec 6>&-
exec 6<> "$dict/clientBrowserLock.fifo"
rm "$dict/clientBrowserLock.fifo"
IFS=''
for((i=0;i<thread_limit;i++)); do
  echo -n " " >&3
done
echo -n " " >&4
echo -n " " >&6

err() {
  read -r -N 1 <&4
  echo -n "$1" >&5
  echo -ne '\0' >&5
  echo -n " " >&4
}
listArr() {
  echo -n "$1"
  shift
  local i=
  for i;do
    echo
    echo -n "$i"
  done
}

trap 'kill -SIGINT %_ 2>/dev/null' SIGINT
trap 'kill -SIGINT %_ 2>/dev/null' EXIT
tgList=()
for system in win-x86 win-x64 linux-arm linux-x64; do
  for proj_type in server client-window client-browser; do
    tgList[${#tgList[@]}]="$proj_type-$system"
  done
done
readarray -t _tgList < <(grep --color=never -E "$filter" - < <(listArr "${tgList[@]}"))
echo "Building: ${_tgList[@]}"
declare -A tgDict
for i in "${_tgList[@]}";do
  tgDict["$i"]=1
done

for system in win-x86 win-x64 linux-arm linux-x64; do
  [ "${tgDict["server-$system"]}" == 1 ] && { # server
    trap 'echo -n " " >&3;exit 130' SIGINT
    read -r -N 1 <&3
    proj_type=server
    dict_proj="$dict/$proj_type-$system"
    echo "Building $proj_type-$system"
    output="$(
      {
        echo "Building Done: $proj_type-$system"
        mkdir -p "$dict_proj/bin"
        trap 'rm -rf "./$dict_proj";echo -n " " >&3;exit 130' SIGINT
        dotnet publish -r "$system" --output "$dict_proj/bin" -p:Version="$ver" -- "$proj_type" || err "Failed to build $proj_type-$system"$'\n'
        [ "${system%%-*}" == win ] && echo $'@echo off\nbin\\'"$proj_type"$'.exe %*\npause' > "$dict_proj/$proj_type.bat"
        [ "${system%%-*}" == linux ] && {
          echo 'bin/'"$proj_type"' "$@"' > "$dict_proj/$proj_type"
          chmod +x "$dict_proj/$proj_type"
        }
        cp $proj_type/config.json "$dict_proj/"
        cp $proj_type/config.schema.json "$dict_proj/"
        trap 'echo -n " " >&3' SIGINT
        cd "$dict"
        zip "$proj_type-$system.zip" -q9r "$proj_type-$system/"
        # rm -rf "./$proj_type-$system"
      } 2>&1
    )"
    read -r -N 1 <&4
    echo "$output"
    echo -n " " >&4
    echo -n ' ' >&3
  } &
  [ "${tgDict["client-window-$system"]}" == 1 ] && { # client-window
    trap 'echo -n " " >&3;exit 130' SIGINT
    read -r -N 1 <&3
    proj_type=client-window
    dict_proj="$dict/$proj_type-$system"
    echo "Building $proj_type-$system"
    output="$(
      {
        echo "Building Done: $proj_type-$system"
        mkdir -p "$dict_proj/bin"
        trap 'rm -rf "./$dict_proj";echo -n " " >&3;exit 130' SIGINT
        dotnet publish -r "$system" --output "$dict_proj/bin" -p:Version="$ver" -- "$proj_type" || err "Failed to build $proj_type-$system"$'\n'
        [ "${system%%-*}" == win ] && echo $'@echo off\nbin\\'"$proj_type"$'.exe %*\npause' > "$dict_proj/$proj_type.bat"
        [ "${system%%-*}" == linux ] && {
          echo 'bin/'"$proj_type"' "$@"' > "$dict_proj/$proj_type"
          chmod +x "$dict_proj/$proj_type"
        }
        trap 'echo -n " " >&3' SIGINT
        cd "$dict"
        zip "$proj_type-$system.zip" -q9r "$proj_type-$system/"
        # rm -rf "./$proj_type-$system"
      } 2>&1
    )"
    read -r -N 1 <&4
    echo "$output"
    echo -n " " >&4
    echo -n ' ' >&3
  } &
  [ "${tgDict["client-browser-$system"]}" == 1 ] && { # client-browser
    trap 'echo -n " " >&3;exit 130' SIGINT
    read -r -N 1 <&3
    read -r -N 1 <&6
    proj_type=client-browser
    dict_proj="$dict/$proj_type-$system"
    echo "Building $proj_type-$system"
    output="$(
      {
        echo "Building Done: $proj_type-$system"
        mkdir -p "$dict_proj/wwwroot"
        trap 'rm -rf "./$dict_proj";echo -n " " >&3;exit 130' SIGINT
        dotnet publish "$proj_type/" -c Release -o "$dict_proj/app" -p:Version="$ver" || err "Failed to build $proj_type-$system"$'\n'
        cp -r "$proj_type/wwwroot" "$dict_proj/"
        [ "${system%%-*}" == win ] && {
          echo $'@echo off\ndotnet serve -d:app\wwwroot %*' > "$dict_proj/$proj_type.bat"
          echo "Use $proj_type.bat -p:PORT -a ADDRESS to serve. It requires dotnet serve(dotnet tool install --global dotnet-serve)" > "$dict_proj/README.txt"
        }
        [ "${system%%-*}" == linux ] && {
          echo 'dotnet serve -d:app/wwwroot "$@"' > "$dict_proj/$proj_type"
          chmod +x "$dict_proj/$proj_type"
          echo "Use $proj_type -p:PORT -a ADDRESS to serve. It requires dotnet serve(dotnet tool install --global dotnet-serve)" > "$dict_proj/README.txt"
        }
        trap 'echo -n " " >&3' SIGINT
        cd "$dict"
        zip "$proj_type-$system.zip" -q9r "$proj_type-$system/"
        # rm -rf "./$proj_type-$system"
      } 2>&1
    )"
    echo -n " " >&6
    read -r -N 1 <&4
    echo "$output"
    echo -n " " >&4
    echo -n ' ' >&3
  } &
done
failed=0
while true;do
  [ "$(jobs -pr)" == '' ] && {
    echo $'\e[34mRelease is fully created.\e[0m'
    exit
  }
  read -r -N 1 <&4
  read -r -d $'\0' -t 0.5 err <&5 && {
    echo -n $'\e[31m'"$err"$'\e[0m'
    echo 'Press Enter to abort building.'
    failed=1
  }
  [ "$failed" == 1 ] && read -r -t 0.1 -d $'\n' && exit 1
  echo -n " " >&4
done