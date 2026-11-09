FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /source
COPY . .
RUN dotnet publish Web.csproj -c Release -o /output
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
WORKDIR /app
COPY --from=build /output .
ARG PROOF_VERSION
RUN test -n "$PROOF_VERSION" && printf '%s' "$PROOF_VERSION" > proof.txt
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Web.dll"]
