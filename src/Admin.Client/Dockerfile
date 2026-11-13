FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["src/Shared.Contracts/Shared.Contracts.csproj", "src/Shared.Contracts/"]
COPY ["src/Admin.Client/Admin.Client.csproj", "src/Admin.Client/"]
RUN dotnet restore "src/Admin.Client/Admin.Client.csproj"
COPY ["src/Shared.Contracts/", "src/Shared.Contracts/"]
COPY ["src/Admin.Client/", "src/Admin.Client/"]
WORKDIR "/src/src/Admin.Client"
RUN dotnet publish "Admin.Client.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Admin.Client.dll"]
