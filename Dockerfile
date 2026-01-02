FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY azure-firewall-updater.sln ./
COPY firewall-updater/firewall-updater.csproj ./firewall-updater/
RUN dotnet restore

COPY . .
RUN dotnet publish firewall-updater -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "firewall-updater.dll"]
