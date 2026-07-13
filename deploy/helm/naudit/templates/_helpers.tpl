{{/* Chart-Name */}}
{{- define "naudit.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/* Vollqualifizierter Name: Release-Name, ggf. um den Chart-Namen ergaenzt */}}
{{- define "naudit.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/* Gemeinsame Labels */}}
{{- define "naudit.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{ include "naudit.selectorLabels" . }}
{{- end -}}

{{/* Selektor-Labels (stabil — nicht nachtraeglich aendern) */}}
{{- define "naudit.selectorLabels" -}}
app.kubernetes.io/name: {{ include "naudit.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/* Name des Bootstrap-Secrets: existierendes oder chart-verwaltetes */}}
{{- define "naudit.bootstrapSecretName" -}}
{{- if .Values.bootstrap.existingSecret -}}
{{- .Values.bootstrap.existingSecret -}}
{{- else -}}
{{- printf "%s-bootstrap" (include "naudit.fullname" .) -}}
{{- end -}}
{{- end -}}
