FROM mcr.microsoft.com/dotnet/sdk:5.0-focal
RUN mkdir /application/
COPY WardenBot /application/WardenBot
COPY entrypoint.sh /application/entrypoint.sh
WORKDIR /application/
ENTRYPOINT [ "/application/entrypoint.sh" ]