pipeline {
    agent {
        docker {
            image 'mcr.microsoft.com/dotnet/sdk:8.0'
            args '-u root:root'
        }
    }

    stages {
        stage('Checkout') { steps { checkout scm } }

        stage('Restore')  { steps { sh "dotnet restore AccessAPP.sln" } }

        stage('Build')    { steps { sh "dotnet build AccessAPP.sln -c Release --no-restore -p:UseAppHost=false" } }

        stage('Publish')  { steps { sh "dotnet publish AccessAPP/AccessAPP.csproj -c Release -o publish -p:UseAppHost=false" } }

        stage('Deploy DEV') {
            steps {
                echo "Deploying to DEV..."
              
            }
        }

        stage('Deploy STAGING') {
            steps {
                input message: 'Deploy to STAGING?', ok: 'Yes'
                echo "Deploying to STAGING..."
                
            }
        }

        stage('Deploy PROD') {
            when {
                branch 'master' // or 'main'
            }
            steps {
                input message: 'Deploy to PROD?', ok: 'Yes, go!'
                echo "Deploying to PROD..."
                
            }
        }
    }
}
