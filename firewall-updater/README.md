## Configuration

### Local developmen

`dotnet user-secrets list`

```
dotnet user-secrets set "FirewallDefinitions:0:password" ""
dotnet user-secrets set "FirewallDefinitions:1:password" ""
```

## Service principal

```
# GIGPIN
az ad sp create-for-rbac --name "azure-firewall-updater"

export APP_ID=d72a749b-3ad7-4737-8291-7e02c7adbdc1
export SUBSCRIPTION=1513ffea-c4f0-4cb4-8585-920e3bc3d1aa
export RESOURCE_GROUP=gigpin
export SERVER_NAME=gigpinapp

az role assignment create \
  --assignee $APP_ID \
  --role "Reader" \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Sql/servers/$SERVER_NAME"

az role assignment create \
  --assignee $APP_ID \
  --role "SQL Security Manager" \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Sql/servers/$SERVER_NAME"

# EMGUEST

az login --tenant 7d7bc7dd-2ee9-4fb5-9e0c-4ea8b513d316

export APP_ID=e9822e9d-bd17-415d-af93-33981873fd10
export SUBSCRIPTION=23d3c028-ad55-44fe-942b-f9953cf8df30
export RESOURCE_GROUP=hotelcms
export SERVER_NAME=hotelcms

az role assignment create \
  --assignee $APP_ID \
  --role "Reader" \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Sql/servers/$SERVER_NAME"

az role assignment create \
  --assignee $APP_ID \
  --role "SQL Security Manager" \
  --scope "/subscriptions/$SUBSCRIPTION/resourceGroups/$RESOURCE_GROUP/providers/Microsoft.Sql/servers/$SERVER_NAME"
```
