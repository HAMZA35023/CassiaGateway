pipeline {
    agent {
        docker {
            image 'mcr.microsoft.com/dotnet/sdk:8.0'  // or 7.0 if you use that
            args '-u root:root'                       // lets you install extras if needed
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
                sh "dotnet build AccessAPP.sln --configuration Release --no-restore"
            }
        }

        stage('Publish') {
            steps {
                // adjust csproj name/path if needed
                sh "dotnet publish AccessAPP/AccessAPP.csproj --configuration Release --output publish --no-build"
            }
        }
    }
}
