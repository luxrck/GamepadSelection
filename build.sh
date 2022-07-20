#!/bin/bash

conf=${1:-Debug}
dotnet.exe publish --configuration $conf
