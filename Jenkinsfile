pipeline {
    agent any

    stages {
        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Restore') {
            steps {
                sh "dotnet restore AccessAPP.sln"
            }
        }

        stage('Build') {
            steps {
                sh "dotnet build AccessAPP.sln --configuration Release --no-restore"
            }
        }

        stage('Publish') {
            steps {
                sh "dotnet publish AccessAPP/AccessAPP.csproj --configuration Release --output publish"
            }
        }
    }
}
