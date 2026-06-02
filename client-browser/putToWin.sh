#! /bin/bash
make clean;make publish;rm -rf /mnt/d/worksp/operation-vote/wwwroot/*;cp -r dist/wwwroot/* /mnt/d/worksp/operation-vote/wwwroot
