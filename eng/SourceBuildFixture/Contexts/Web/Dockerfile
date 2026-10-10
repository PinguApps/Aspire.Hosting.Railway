FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish Web.csproj -c Release -o /output
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /output .
ARG PROOF_VERSION
RUN test -n "$PROOF_VERSION" && printf '%s' "$PROOF_VERSION" > proof.txt
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Web.dll"]
