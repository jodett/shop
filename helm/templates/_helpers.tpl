{{- define "shopware-k3s.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "shopware-k3s.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "shopware-k3s.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "shopware-k3s.labels" -}}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version | replace "+" "_" }}
app.kubernetes.io/name: {{ include "shopware-k3s.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{- define "shopware-k3s.selectorLabels" -}}
app.kubernetes.io/name: {{ include "shopware-k3s.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{- define "shopware-k3s.serviceAccountName" -}}
{{- if .Values.serviceAccount.create -}}
{{ default (include "shopware-k3s.fullname" .) .Values.serviceAccount.name }}
{{- else -}}
{{ default "default" .Values.serviceAccount.name }}
{{- end -}}
{{- end -}}

{{- define "shopware-k3s.databaseUrl" -}}
{{- printf "mysql://%s:%s@%s:%v/%s" .Values.mariadb.user .Values.mariadb.password .Values.mariadb.host (.Values.mariadb.port | int) .Values.mariadb.database -}}
{{- end -}}
