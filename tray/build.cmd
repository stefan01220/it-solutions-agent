@echo off
rem Baut IT-Solutions-Agent.exe mit dem C#-Compiler aus .NET Framework 4.x (kein SDK noetig).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:IT-Solutions-Agent.exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll TrayAgent.cs
exit /b %ERRORLEVEL%
