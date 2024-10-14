#!/usr/bin/bash

set -e

DIR="$(dirname "${BASH_SOURCE[0]}")"

if [[ -d $HOME/.local/share/tempo-reporter ]]; then
  rm -r $HOME/.local/share/tempo-reporter/*
fi
dotnet publish $DIR/src/tempo-reporter/tempo-reporter.csproj -c Release -o $HOME/.local/share/tempo-reporter
ln -fs $HOME/.local/share/tempo-reporter/tempo-reporter $HOME/.local/bin/tempo-reporter
