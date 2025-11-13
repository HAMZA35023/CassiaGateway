pipeline {
    agent {
        docker {
            image 'mcr.microsoft.com/dotnet/sdk:8.0'
            args '-u root:root'
        }
    }

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
                sh "dotnet build AccessAPP.sln --configuration Release --no-restore -p:UseAppHost=false"
            }
        }

        stage('Publish') {
            steps {
                sh "dotnet publish AccessAPP/AccessAPP.csproj --configuration Release --output publish --no-build -p:UseAppHost=false"
            }
        }
    }
}
