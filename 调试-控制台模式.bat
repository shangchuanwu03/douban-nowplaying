@echo off
cd /d "%~dp0"
echo Starting local server on http://127.0.0.1:4590 ...
echo Close this window to stop the server.
node server\index.js
